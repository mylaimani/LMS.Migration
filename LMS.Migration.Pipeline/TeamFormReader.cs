using ClickHouse.Client.ADO;

namespace LMS.Migration.Worker
{
    /// <summary>
    /// By Mani 21 Oct 2026
    /// Form score of a team BEFORE a given match date, read from ClickHouse.
    /// Same rule as FormTracker (spec §3.2): last 6 results, Win = +1, Loss = -1,
    /// Tie / No Result = 0, score = sum ÷ 6. FormTracker needs every fixture in
    /// chronological order inside one run; this reader gives the same answer for a
    /// single fixture arriving on its own, which is what the Ball Worker needs.
    /// </summary>
    public class TeamFormReader
    {
        private const int Window = 6;
        private readonly string _connectionString;
        private readonly string _database;

        public TeamFormReader(string connectionString, string databaseName = "LMSClickHouseDB")
        {
            _connectionString = connectionString;
            _database = databaseName;
        }

        public async Task<float> FormScoreBeforeAsync(uint teamId, DateTime gameDate)
        {
            using var connection = new ClickHouseConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = $@"
                SELECT match_result
                FROM (
                    SELECT DISTINCT fixture_id, game_date, match_result
                    FROM {_database}.player_match_stats
                    WHERE team_id = {teamId} AND game_date < '{gameDate:yyyy-MM-dd}'
                )
                ORDER BY game_date DESC, fixture_id DESC
                LIMIT {Window}";

            int sumOfResults = 0;
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                sumOfResults += ResultValue(reader.GetString(0));

            return sumOfResults / (float)Window;
        }

        private static int ResultValue(string matchResult)
        {
            if (matchResult == "Win") return 1;
            if (matchResult == "Loss") return -1;
            return 0;
        }
    }
}
