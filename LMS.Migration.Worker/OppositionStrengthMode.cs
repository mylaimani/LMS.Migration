// By Mani
using LMS.Migration.Core.OppositionStrength;

namespace LMS.Migration.Worker
{
    /// <summary>
    /// Test mode:  .\LMS.Migration.Worker.exe opposition 515388 486870
    ///
    /// Calculates Category 1 (ball-by-ball opposition uplift) for the given fixtures and writes the
    /// rows to player_opposition_strength. Uses each opponent's rating and reliability as it was BEFORE the
    /// fixture, from Rep_PlayerRatingHistory; rows are marked rating_source = 'BeforeFixture'. Balls come from ClickHouse
    /// ball_events, so FixtureState in SQL Server is never read.
    ///
    /// Needs:  setx LMS_RATING_SQL_CONN "Server=...;Database=<staging db>;..."  (the DB with Rep_PlayerRatingHistory)
    /// </summary>
    public static class OppositionStrengthMode
    {
        public const string BeforeFixtureRatingSource = "BeforeFixture";

        public static async Task RunAsync(string[] args, string clickHouseConnectionString, string clickHouseDatabase, ClickHouseWriter writer)
        {
            string? ratingSqlConnectionString = Environment.GetEnvironmentVariable("LMS_RATING_SQL_CONN");
            if (string.IsNullOrEmpty(ratingSqlConnectionString))
            {
                Console.WriteLine("Environment variable LMS_RATING_SQL_CONN is not set (staging DB with Rep_PlayerRatingHistory).");
                return;
            }

            List<uint> fixtureIds = ReadFixtureIdsFromArgs(args);
            if (fixtureIds.Count == 0)
            {
                Console.WriteLine("Usage: LMS.Migration.Worker.exe opposition <fixtureId> [<fixtureId> ...]");
                return;
            }

            var reader = new OppositionStrengthReader(clickHouseConnectionString, clickHouseDatabase, ratingSqlConnectionString);
            var allRows = new List<PlayerOppositionStrengthRow>();
            foreach (uint fixtureId in fixtureIds)
            {
                List<PlayerOppositionStrengthRow> rowsForFixture = await CalculateFixtureAsync(reader, fixtureId);
                allRows.AddRange(rowsForFixture);
            }

            await writer.InsertPlayerOppositionStrengthAsync(allRows);
            Console.WriteLine($"Done. {allRows.Count} rows written to player_opposition_strength.");
        }

        private static async Task<List<PlayerOppositionStrengthRow>> CalculateFixtureAsync(OppositionStrengthReader reader, uint fixtureId)
        {
            List<BatsmanBowlerBalls> ballsPerPair = await reader.ReadBallsPerBatsmanAndBowlerAsync(fixtureId);
            if (ballsPerPair.Count == 0)
            {
                Console.WriteLine($"[SKIP] {fixtureId} - no balls in ball_events");
                return new List<PlayerOppositionStrengthRow>();
            }

            List<uint> playerIds = CollectPlayerIds(ballsPerPair);
            Dictionary<uint, PlayerRatingSnapshot> ratingsByPlayerId = await reader.ReadRatingsBeforeFixtureAsync(fixtureId, playerIds);

            List<PlayerOppositionStrengthRow> rows = OppositionStrengthBuilder.BuildForFixture(
                ballsPerPair, ratingsByPlayerId, BeforeFixtureRatingSource, DateTime.UtcNow);

            Console.WriteLine($"[OK] {fixtureId} - {playerIds.Count} players, {ratingsByPlayerId.Count} with ratings, {rows.Count} rows");
            PrintRows(rows);
            return rows;
        }

        private static List<uint> ReadFixtureIdsFromArgs(string[] args)
        {
            var fixtureIds = new List<uint>();
            for (int index = 1; index < args.Length; index++)
            {
                if (uint.TryParse(args[index], out uint fixtureId))
                {
                    fixtureIds.Add(fixtureId);
                }
                else
                {
                    Console.WriteLine($"Ignoring '{args[index]}' - not a fixture id.");
                }
            }
            return fixtureIds;
        }

        private static List<uint> CollectPlayerIds(List<BatsmanBowlerBalls> ballsPerPair)
        {
            var playerIds = new HashSet<uint>();
            foreach (BatsmanBowlerBalls pair in ballsPerPair)
            {
                playerIds.Add(pair.BatsmanId);
                playerIds.Add(pair.BowlerId);
            }
            return playerIds.ToList();
        }

        private static void PrintRows(List<PlayerOppositionStrengthRow> rows)
        {
            foreach (PlayerOppositionStrengthRow row in rows)
            {
                Console.WriteLine($"    {row.Role,-8} player {row.PlayerId,-8} balls {row.BallsCounted,3}  " +
                                  $"opponents {row.OpponentsCounted} (no rating {row.OpponentsWithoutRating})  " +
                                  $"rating {row.EffectiveOppositionRating,5}  uplift {row.UpliftPercent,7}%");
            }
        }
    }
}
// End of Mani
